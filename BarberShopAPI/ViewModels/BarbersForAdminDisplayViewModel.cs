using BarberShopAPI.Models;
using System.ComponentModel.DataAnnotations;

namespace BarberShopAPI.ViewModels
{
    public class BarbersForAdminDisplayViewModel
    {
        public int Id { get; set; }
        public string FirstName { get; set; }
        public string? LastName { get; set; }
        public string ImageUrl { get; set; }
        public int TotalBookings { get; set; }
    }
}
