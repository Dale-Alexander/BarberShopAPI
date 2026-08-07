using BarberShopAPI.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;



namespace BarberShopAPI.Models
{
    [Index(nameof(BarberId), nameof(StartDateTime), IsUnique = true)]
    [Index(nameof(PublicId), IsUnique = true)]
    public class Booking
    {
        [Key]
        /* [Key]: Marks this property as the primary ke of the table
         * By default, it is auto-incremented
         * */
        public int Id { get; set; }

        /* Non-guessable public identifier (32-char hex, Guid "N") used in guest-facing URLs
         * (/checkout, /booking/success, /cancelledorcompleted) and the guest payment endpoints instead
         * of the sequential int Id. Stops URL id-enumeration (IDOR) - e.g. reading another customer's
         * confirmation via a guessed id. The int Id stays internal: DB relations, Stripe metadata, and
         * the admin [Authorize] endpoints all keep using it. */
        [MaxLength(32)]
        public string PublicId { get; set; }
        public DateTime StartDateTime { get; set; }
        public BookingStatus Status { get; set; } = BookingStatus.PENDING;
        public int? UserId { get; set; }
        /* The above line means nullable integer. Sometimes a Booking might
         * temporarily not have a user assigned. int? allows null*/ 
        [ForeignKey("UserId")]
        public virtual User? User { get; set; }
        /* the above 2 lines tell EF Core: "The property UserId is the foreign key
         * for the navigation property User.
         EF Core usually guesses foreign keys automatically, but if you want explicit mapping
        or the naming isnt standard(UserId), you can implement it. But i think you can
        remove it
        Booking.UserId -> stores the foreign key in the database
        Booking.User -> lets you access the related User Object
        
        You could do it ike this:
            public int? UserId { get; set; } // foreign key
            public User User { get; set; }
        This works because EF Core will automatically detect UserId as the foreign key
        because the format is like this: <ModelName>Id. Use ForeignKey(...)
        if the format isnt like the one previously mentioned above
         */
        public int BarberId { get; set; }
        [ForeignKey("BarberId")]
        public virtual Barber Barber { get; set; }
        public virtual Payment? Payment { get; set; }
        public virtual ICollection<BookingService> Services { get; set; }
        public int DurationMin { get; set; }

        public string? ReminderJobId { get; set; }
        /* the reason why it is not an IQueryable is because IQueryable
         * is for queries, not storage. */

        /* public virtual ...:
         * "virtual" enables lazy loading in EF Core. Lazy loading means EF
         Core will only load the virtual attribute from the database when you 
        actually access the property, saving memory and queries if you dont 
        need it

        Whenever you have ICollection<Model> that means it is a one-many 
        relationship
         */

        public DateTime? ReminderSentAt { get; set; }
        public DateTime? ConfirmationSentAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? ContactEmail { get; set; }
        public string? StripePaymentIntentId { get; set; }

        /* Set when an automated money-vs-booking reconciliation couldn't complete on its own and a human
         * has to finish it by hand (e.g. a closure landed on a paid booking but the Stripe refund failed,
         * so the booking is neither properly cancelled nor refunded). ReviewReason carries the same context
         * the log line does so the admin knows what to check in Stripe. Cleared via the mark-reviewed
         * endpoint once the money and status have been reconciled. */
        public bool NeedsReview { get; set; } = false;
        public string? ReviewReason { get; set; }

        /* Staff deliberately placed this booking outside the shop's opening hours / outside the assigned
         * barber's shifts. Set only on the staff paths (a customer can never reach either), and always
         * RECOMPUTED on reschedule rather than carried - a booking moved back into normal hours must stop
         * being exempt, or it is excused from every future check for the rest of its life.
         *
         * Two flags, not one, because the two are independent: staff routinely book outside one barber's
         * shift while staying well inside shop hours, and a single "overridden" bit would then also excuse
         * that booking from a later SHOP-hours check - where being outside really would be an accident
         * nobody agreed to.
         *
         * They exist to answer the question the conflict sweeps could not: a booking sitting outside the
         * hours says nothing on its own about whether the hours moved under it or someone put it there on
         * purpose. Without them, SchedulesController.FindConflictsAsync reported every deliberate booking
         * as stranded at every schedule edit that touched its date, forever - it can never clear itself,
         * because FindBackInsideHoursAsync only rescues bookings that land back INSIDE the new hours. */
        public bool OutsideShopHours { get; set; } = false;
        public bool OutsideBarberSchedule { get; set; } = false;

        /* Who confirmed that override, taken from the caller's token - never asked for, so it can't be
         * skipped or mistyped. Null when nothing was overridden. Kept because an override commits SOMEONE
         * ELSE to work: an admin booking a barber outside their shift is exactly the decision that gets
         * disputed later, and "make sure they've agreed" is already what the confirmation says. */
        public int? OverriddenByUserId { get; set; }
        [ForeignKey("OverriddenByUserId")]
        public virtual User? OverriddenBy { get; set; }

        /* Why this booking was cancelled (None until it is). Drives the customer-facing cancelled screen
         * and the cancellation email wording - e.g. BarberUnavailable when the assigned barber is
         * deactivated, ShopClosure when a closure lands on the slot. See CancellationReason. */
        public CancellationReason CancellationReason { get; set; } = CancellationReason.None;

        /* How to know its a navigation property: 
         *DataType is another model class. For example: Payment, Barber etc
         *Please note that navigation properties are not stored as real
         *columns. They exist in your C# class to let you work with related
         *entites easily. 
         *
         *Without navigational properties you would have to find the Id first like this:
         *var booking = context.Bookings.Find(id);
          var user = context.Users.Find(booking.UserId);
        You dont have to add a navigation property for every relationship, but it's
        best practice for maintainability.
         */
    }
}
