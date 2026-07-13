using BarberShopAPI.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;



namespace BarberShopAPI.Models
{
    [Index(nameof(BarberId), nameof(StartDateTime), IsUnique = true)]
    public class Booking
    {
        [Key]
        /* [Key]: Marks this property as the primary ke of the table
         * By default, it is auto-incremented
         * */
        public int Id { get; set; }
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
