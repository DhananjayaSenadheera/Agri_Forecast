using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;

// GET /api/portfolio/dashboard — one item per crop the CALLER watches: latest observed price + trend at
// their home market (economic-centre fallback, flagged) and the newest frozen forecast snapshot.
//
// UserId is stamped by the controller from the JWT subject, never bound from the request. Read-only: this
// query never writes, and in particular never touches ForecastSnapshots, which the nightly Python job owns.
public class GetPortfolioDashboardQuery : IRequest<Result<PortfolioDashboard_GetDto>>
{
    public Guid UserId { get; set; }
}
